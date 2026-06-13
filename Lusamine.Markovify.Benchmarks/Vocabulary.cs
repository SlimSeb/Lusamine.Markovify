namespace Lusamine.Markovify.Benchmarks;

/// <summary>
/// Maps each distinct word to a small integer id and back. Interning every word
/// exactly once is the core of the refactor: the original <see cref="Chain"/>
/// keeps a separate <see cref="string"/> object for every occurrence of a word
/// across every state and follower table, so a corpus that uses the word "the"
/// a million times holds (transitively) a million references and many duplicate
/// string objects. Here each unique word is stored once and referred to by an int.
/// </summary>
internal sealed class Vocabulary
{
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly List<string> _words = new();

    /// <summary>Number of distinct words seen so far.</summary>
    public int Count => _words.Count;

    /// <summary>Returns the id for <paramref name="word"/>, assigning a new one if unseen.</summary>
    public int Intern(string word)
    {
        if (_ids.TryGetValue(word, out var id))
            return id;

        id = _words.Count;
        _ids[word] = id;
        _words.Add(word);
        return id;
    }

    /// <summary>Returns the id for <paramref name="word"/>, or <c>-1</c> if it was never interned.</summary>
    public int Lookup(string word) => _ids.TryGetValue(word, out var id) ? id : -1;

    /// <summary>Returns the word for a previously assigned id.</summary>
    public string Word(int id) => _words[id];
}
