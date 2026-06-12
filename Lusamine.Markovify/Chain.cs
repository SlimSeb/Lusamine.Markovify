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

    private readonly Dictionary<State, Dictionary<string, int>> _model;

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
    public IReadOnlyDictionary<State, Dictionary<string, int>> Model => _model;

    private Chain(Dictionary<State, Dictionary<string, int>> model, int stateSize)
    {
        _model = model;
        StateSize = stateSize;
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

        return new Chain(model, stateSize);
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

            var choices = new string[followers.Count];
            var cumDist = new double[followers.Count];
            var sum = 0.0;
            var index = 0;
            foreach (var (word, count) in followers)
            {
                choices[index] = word;
                // At T == 1 the weight is exactly the observed count; otherwise it is
                // count^(1/T), which flattens (T > 1) or sharpens (T < 1) the distribution.
                sum += _temperature.Equals(1.0) ? count : Math.Pow(count, 1.0 / _temperature);
                cumDist[index] = sum;
                index++;
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

                foreach (var (word, count) in followers)
                {
                    var scaled = (int)Math.Round(count * weight);
                    if (scaled <= 0)
                        continue;
                    target[word] = target.GetValueOrDefault(word) + scaled;
                }
            }
        }

        return new Chain(merged, stateSize);
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
            foreach (var (word, count) in followers)
                writer.WriteNumber(word, count);
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
        var model = new Dictionary<State, Dictionary<string, int>>();
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

            var followers = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var follower in pair[1].EnumerateObject())
                followers[follower.Name] = follower.Value.GetInt32();

            model[new State(words)] = followers;
        }

        if (stateSize == -1)
            throw new ArgumentException("Serialized chain contained no states.", nameof(root));

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
