using System.Text;
using System.Text.Json;

namespace Lusamine.Markovify;

/// <summary>
/// A Markov chain representing processes that have both beginnings and ends.
/// For example: sentences. This is a faithful C# port of the core of Python's
/// <c>markovify</c> <c>Chain</c> class.
/// </summary>
public sealed class Chain
{
    /// <summary>Sentinel token marking the beginning of a run.</summary>
    public const string Begin = "___BEGIN__";

    /// <summary>Sentinel token marking the end of a run.</summary>
    public const string End = "___END__";

    private readonly Dictionary<State, Followers> _model;

    // Lazily-built sampling tables: state -> (followers, cumulative weights).
    private readonly Dictionary<State, (string[] Choices, double[] CumDist)> _cache = new();

    private double _temperature = 1.0;

    /// <summary>The number of words that make up each state.</summary>
    public int StateSize { get; }

    /// <summary>
    /// Sampling temperature applied when walking the chain. <c>1.0</c> (the default)
    /// reproduces the trained distribution. Values greater than <c>1.0</c> flatten it,
    /// making rarer followers more likely and reducing verbatim reproduction of the
    /// source. Values in <c>(0, 1)</c> sharpen it toward the most common follower.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not greater than zero.</exception>
    public double Temperature
    {
        get => _temperature;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Temperature must be greater than zero.");
            if (value.Equals(_temperature))
                return;
            _temperature = value;
            // Cached cumulative weights depend on the temperature; force a rebuild.
            _cache.Clear();
        }
    }

    /// <summary>The raw transition model: state -> (follower -> count).</summary>
    public IReadOnlyDictionary<State, Followers> Model => _model;

    private Chain(Dictionary<State, Followers> model, int stateSize)
    {
        _model = model;
        StateSize = stateSize;
    }

    // Converts a mutable count map (used only while accumulating) into the compact
    // array-backed model. The source is drained as it goes so the bulky per-state
    // dictionaries are released for collection rather than coexisting with the result.
    private static Dictionary<State, Followers> Compact(Dictionary<State, Dictionary<string, int>> counts)
    {
        var model = new Dictionary<State, Followers>(counts.Count);
        foreach (var state in counts.Keys.ToArray())
        {
            var followers = counts[state];
            counts.Remove(state);

            var words = new string[followers.Count];
            var weights = new int[followers.Count];
            var index = 0;
            foreach (var (word, count) in followers)
            {
                words[index] = word;
                weights[index] = count;
                index++;
            }

            model[state] = new Followers(words, weights);
        }

        return model;
    }

    /// <summary>
    /// Builds a chain from a corpus, where each element of <paramref name="corpus"/>
    /// is an already-tokenized run (e.g. the words of a sentence).
    /// </summary>
    public static Chain Build(IEnumerable<IReadOnlyList<string>> corpus, int stateSize)
    {
        if (stateSize < 1)
            throw new ArgumentOutOfRangeException(nameof(stateSize), "State size must be at least 1.");

        var model = new Dictionary<State, Dictionary<string, int>>();

        foreach (var run in corpus)
        {
            // items = [BEGIN] * stateSize + run + [END]
            var items = new string[stateSize + run.Count + 1];
            for (var i = 0; i < stateSize; i++)
                items[i] = Begin;
            for (var i = 0; i < run.Count; i++)
                items[stateSize + i] = run[i];
            items[^1] = End;

            for (var i = 0; i <= run.Count; i++)
            {
                var stateWords = new string[stateSize];
                Array.Copy(items, i, stateWords, 0, stateSize);
                var state = new State(stateWords);
                var follow = items[i + stateSize];

                if (!model.TryGetValue(state, out var followers))
                {
                    followers = new Dictionary<string, int>(StringComparer.Ordinal);
                    model[state] = followers;
                }

                followers[follow] = followers.GetValueOrDefault(follow) + 1;
            }
        }

        return new Chain(Compact(model), stateSize);
    }

    /// <summary>The all-<see cref="Begin"/> state from which walks start by default.</summary>
    public State BeginState()
    {
        var words = new string[StateSize];
        Array.Fill(words, Begin);
        return new State(words);
    }

    /// <summary>
    /// Randomly selects the word that follows <paramref name="state"/>, weighted by
    /// the counts observed during training. May return <see cref="End"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The state was never seen during training.</exception>
    public string Move(State state, Random rng)
    {
        if (!_cache.TryGetValue(state, out var entry))
        {
            if (!_model.TryGetValue(state, out var followers))
                throw new KeyNotFoundException($"State {state} is not present in the model.");

            var words = followers.WordsArray;
            var counts = followers.CountsArray;
            var choices = new string[words.Length];
            var cumDist = new double[words.Length];
            var sum = 0.0;
            for (var index = 0; index < words.Length; index++)
            {
                choices[index] = words[index];
                // At T == 1 the weight is exactly the observed count; otherwise it is
                // count^(1/T), which flattens (T > 1) or sharpens (T < 1) the distribution.
                sum += _temperature.Equals(1.0) ? counts[index] : Math.Pow(counts[index], 1.0 / _temperature);
                cumDist[index] = sum;
            }

            entry = (choices, cumDist);
            _cache[state] = entry;
        }

        var total = entry.CumDist[^1];
        var roll = rng.NextDouble() * total;
        var pick = BisectRight(entry.CumDist, roll);
        return entry.Choices[pick];
    }

    /// <summary>
    /// Walks the chain, yielding generated words until (but not including) the
    /// <see cref="End"/> sentinel is reached.
    /// </summary>
    public IEnumerable<string> Walk(State? initState = null, Random? rng = null)
    {
        rng ??= Random.Shared;
        var state = initState ?? BeginState();
        while (true)
        {
            var next = Move(state, rng);
            if (next == End)
                yield break;
            yield return next;
            state = state.Shift(next);
        }
    }

    /// <summary>
    /// Combines several chains into one, summing each follower count after scaling
    /// by the corresponding weight. All chains must share the same state size.
    /// </summary>
    public static Chain Combine(IReadOnlyList<Chain> chains, IReadOnlyList<double>? weights = null)
    {
        if (chains.Count == 0)
            throw new ArgumentException("At least one chain is required.", nameof(chains));

        weights ??= Enumerable.Repeat(1.0, chains.Count).ToArray();
        if (weights.Count != chains.Count)
            throw new ArgumentException("Number of weights must match number of chains.", nameof(weights));

        var stateSize = chains[0].StateSize;
        if (chains.Any(c => c.StateSize != stateSize))
            throw new ArgumentException("All chains must have the same state size.", nameof(chains));

        var merged = new Dictionary<State, Dictionary<string, int>>();
        for (var i = 0; i < chains.Count; i++)
        {
            var weight = weights[i];
            foreach (var (state, followers) in chains[i]._model)
            {
                if (!merged.TryGetValue(state, out var target))
                {
                    target = new Dictionary<string, int>(StringComparer.Ordinal);
                    merged[state] = target;
                }

                var words = followers.WordsArray;
                var counts = followers.CountsArray;
                for (var j = 0; j < words.Length; j++)
                {
                    var scaled = (int)Math.Round(counts[j] * weight);
                    if (scaled <= 0)
                        continue;
                    target[words[j]] = target.GetValueOrDefault(words[j]) + scaled;
                }
            }
        }

        return new Chain(Compact(merged), stateSize);
    }

    /// <summary>
    /// Removes rare transitions: any follower observed fewer than <paramref name="minCount"/>
    /// times is dropped, and any state left with no remaining followers is removed entirely.
    /// The sampling cache is invalidated so subsequent walks reflect the pruned model.
    /// </summary>
    /// <param name="minCount">The minimum observed count a follower must have to be kept.</param>
    /// <returns>The number of follower transitions removed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minCount"/> is less than 1.</exception>
    /// <remarks>
    /// Pruning can leave a surviving transition pointing at a state that was itself removed.
    /// Walking into such a state throws <see cref="KeyNotFoundException"/>, which the
    /// generation methods on <see cref="Text"/> already treat as a failed attempt.
    /// </remarks>
    public int Prune(int minCount)
    {
        if (minCount < 1)
            throw new ArgumentOutOfRangeException(nameof(minCount), "Minimum count must be at least 1.");

        var removed = 0;
        var emptyStates = new List<State>();
        // Snapshot the keys because surviving states are reassigned while iterating.
        foreach (var state in _model.Keys.ToArray())
        {
            var words = _model[state].WordsArray;
            var counts = _model[state].CountsArray;

            var keep = 0;
            foreach (var count in counts)
            {
                if (count >= minCount)
                    keep++;
            }

            if (keep == counts.Length)
                continue;

            removed += counts.Length - keep;

            if (keep == 0)
            {
                emptyStates.Add(state);
                continue;
            }

            var newWords = new string[keep];
            var newCounts = new int[keep];
            var index = 0;
            for (var i = 0; i < counts.Length; i++)
            {
                if (counts[i] < minCount)
                    continue;
                newWords[index] = words[i];
                newCounts[index] = counts[i];
                index++;
            }

            _model[state] = new Followers(newWords, newCounts);
        }

        foreach (var state in emptyStates)
            _model.Remove(state);

        if (removed > 0)
            _cache.Clear();

        return removed;
    }

    /// <summary>Serializes the transition model to JSON.</summary>
    /// <remarks>
    /// Materializes the whole model as a single <see cref="string"/>; for very large
    /// corpora this can exceed the maximum string length. Use <see cref="WriteTo"/>
    /// (or <see cref="Text.Save(string)"/>) to stream directly to a destination instead.
    /// </remarks>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteTo(writer);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes the transition model to <paramref name="writer"/> as a JSON array.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        foreach (var (state, followers) in _model)
        {
            writer.WriteStartArray();

            writer.WriteStartArray();
            foreach (var word in state.Words)
                writer.WriteStringValue(word);
            writer.WriteEndArray();

            writer.WriteStartObject();
            var words = followers.WordsArray;
            var counts = followers.CountsArray;
            for (var i = 0; i < words.Length; i++)
                writer.WriteNumber(words[i], counts[i]);
            writer.WriteEndObject();

            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    /// <summary>Reconstructs a chain previously produced by <see cref="ToJson"/>.</summary>
    public static Chain FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return FromElement(document.RootElement);
    }

    /// <summary>Reconstructs a chain from an already-parsed JSON array element.</summary>
    public static Chain FromElement(JsonElement root)
    {
        var model = new Dictionary<State, Followers>();
        var stateSize = -1;

        foreach (var pair in root.EnumerateArray())
        {
            var stateElement = pair[0];
            var words = new string[stateElement.GetArrayLength()];
            var index = 0;
            foreach (var word in stateElement.EnumerateArray())
                words[index++] = word.GetString()!;

            if (stateSize == -1)
                stateSize = words.Length;

            // Build the follower arrays directly instead of a transient per-state
            // dictionary, keeping the loaded model compact in memory.
            var followerElement = pair[1];
            var followerCount = 0;
            foreach (var _ in followerElement.EnumerateObject())
                followerCount++;

            var followerWords = new string[followerCount];
            var followerCounts = new int[followerCount];
            var f = 0;
            foreach (var follower in followerElement.EnumerateObject())
            {
                followerWords[f] = follower.Name;
                followerCounts[f] = follower.Value.GetInt32();
                f++;
            }

            model[new State(words)] = new Followers(followerWords, followerCounts);
        }

        if (stateSize == -1)
            throw new ArgumentException("Serialized chain contained no states.", nameof(root));

        return new Chain(model, stateSize);
    }

    /// <summary>
    /// Writes the transition model to <paramref name="writer"/> in a compact binary form.
    /// </summary>
    /// <remarks>
    /// Words are written once into a deduplicated string table and referenced elsewhere by
    /// index, so the typical heavy repetition of common words and the <see cref="Begin"/>
    /// sentinel costs only a small integer per occurrence. Counts and indices use variable
    /// length encoding. The model is streamed straight to <paramref name="writer"/> and is
    /// never materialized as a single string, so it handles corpora of any size.
    /// </remarks>
    public void WriteBinary(BinaryWriter writer)
    {
        // Assign every distinct word a small, stable index. The table is written first so the
        // reader can resolve indices as it streams through the model.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var strings = new List<string>();
        foreach (var (state, followers) in _model)
        {
            foreach (var word in state.Words)
                Intern(word);
            foreach (var word in followers.WordsArray)
                Intern(word);
        }

        writer.Write7BitEncodedInt(StateSize);

        writer.Write7BitEncodedInt(strings.Count);
        foreach (var word in strings)
            writer.Write(word);

        writer.Write7BitEncodedInt(_model.Count);
        foreach (var (state, followers) in _model)
        {
            foreach (var word in state.Words)
                writer.Write7BitEncodedInt(ids[word]);

            var words = followers.WordsArray;
            var counts = followers.CountsArray;
            writer.Write7BitEncodedInt(words.Length);
            for (var i = 0; i < words.Length; i++)
            {
                writer.Write7BitEncodedInt(ids[words[i]]);
                writer.Write7BitEncodedInt(counts[i]);
            }
        }

        return;

        void Intern(string word)
        {
            if (ids.ContainsKey(word))
                return;
            ids[word] = strings.Count;
            strings.Add(word);
        }
    }

    /// <summary>Reconstructs a chain previously written by <see cref="WriteBinary"/>.</summary>
    public static Chain ReadBinary(BinaryReader reader)
    {
        var stateSize = reader.Read7BitEncodedInt();
        if (stateSize < 1)
            throw new InvalidDataException($"Invalid state size {stateSize} in binary chain.");

        var stringCount = reader.Read7BitEncodedInt();
        if (stringCount < 0)
            throw new InvalidDataException($"Invalid string table length {stringCount} in binary chain.");
        var strings = new string[stringCount];
        for (var i = 0; i < stringCount; i++)
            strings[i] = reader.ReadString();

        var stateCount = reader.Read7BitEncodedInt();
        if (stateCount < 0)
            throw new InvalidDataException($"Invalid state count {stateCount} in binary chain.");

        var model = new Dictionary<State, Followers>(stateCount);
        for (var s = 0; s < stateCount; s++)
        {
            var words = new string[stateSize];
            for (var i = 0; i < stateSize; i++)
                words[i] = strings[reader.Read7BitEncodedInt()];

            // Read straight into the compact follower arrays: no transient per-state
            // dictionary, which is what keeps loading a multi-gigabyte model in bounds.
            var followerCount = reader.Read7BitEncodedInt();
            if (followerCount < 0)
                throw new InvalidDataException($"Invalid follower count {followerCount} in binary chain.");
            var followerWords = new string[followerCount];
            var followerCounts = new int[followerCount];
            for (var i = 0; i < followerCount; i++)
            {
                followerWords[i] = strings[reader.Read7BitEncodedInt()];
                followerCounts[i] = reader.Read7BitEncodedInt();
            }

            model[new State(words)] = new Followers(followerWords, followerCounts);
        }

        return new Chain(model, stateSize);
    }

    // bisect.bisect_right: index of the first element strictly greater than value.
    private static int BisectRight(double[] sortedCumulative, double value)
    {
        var low = 0;
        var high = sortedCumulative.Length;
        while (low < high)
        {
            var mid = (low + high) >> 1;
            if (value < sortedCumulative[mid])
                high = mid;
            else
                low = mid + 1;
        }
        return low;
    }
}
