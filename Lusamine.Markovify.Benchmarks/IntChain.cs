namespace Lusamine.Markovify.Benchmarks;

/// <summary>
/// A drop-in-shaped reimplementation of <see cref="Chain"/> that stores word ids
/// instead of strings. The model is <c>state(int ids) -&gt; (follower id -&gt; count)</c>.
/// Behaviour (build, weighted/temperature sampling, walk output) is intended to be
/// equivalent to <see cref="Chain"/>; only the internal representation differs, so
/// the two can be benchmarked head to head on the same corpus.
/// </summary>
internal sealed class IntChain
{
    private readonly Vocabulary _vocab;
    private readonly Dictionary<IntState, Dictionary<int, int>> _model;
    private readonly Dictionary<IntState, (int[] Choices, double[] CumDist)> _cache = new();
    private readonly int _beginId;
    private readonly int _endId;
    private double _temperature = 1.0;

    public int StateSize { get; }

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
            _cache.Clear();
        }
    }

    /// <summary>Number of distinct states (for sanity-checking against <see cref="Chain"/>).</summary>
    public int StateCount => _model.Count;

    private IntChain(Vocabulary vocab, Dictionary<IntState, Dictionary<int, int>> model, int stateSize,
        int beginId, int endId)
    {
        _vocab = vocab;
        _model = model;
        StateSize = stateSize;
        _beginId = beginId;
        _endId = endId;
    }

    public static IntChain Build(IEnumerable<IReadOnlyList<string>> corpus, int stateSize)
    {
        if (stateSize < 1)
            throw new ArgumentOutOfRangeException(nameof(stateSize), "State size must be at least 1.");

        var vocab = new Vocabulary();
        var beginId = vocab.Intern(Chain.Begin);
        var endId = vocab.Intern(Chain.End);
        var model = new Dictionary<IntState, Dictionary<int, int>>();

        foreach (var run in corpus)
        {
            // items = [BEGIN] * stateSize + run (as ids) + [END]
            var items = new int[stateSize + run.Count + 1];
            for (var i = 0; i < stateSize; i++)
                items[i] = beginId;
            for (var i = 0; i < run.Count; i++)
                items[stateSize + i] = vocab.Intern(run[i]);
            items[^1] = endId;

            for (var i = 0; i <= run.Count; i++)
            {
                var stateIds = new int[stateSize];
                Array.Copy(items, i, stateIds, 0, stateSize);
                var state = new IntState(stateIds);
                var follow = items[i + stateSize];

                if (!model.TryGetValue(state, out var followers))
                {
                    followers = new Dictionary<int, int>();
                    model[state] = followers;
                }

                followers[follow] = followers.GetValueOrDefault(follow) + 1;
            }
        }

        return new IntChain(vocab, model, stateSize, beginId, endId);
    }

    /// <summary>
    /// Freezes the model into a read-only CSR <see cref="FrozenIntChain"/>. The returned
    /// chain copies the data into flat arrays and does not retain this instance's
    /// per-state dictionaries, so they become collectable once this chain is dropped.
    /// </summary>
    public FrozenIntChain Freeze() => new(_vocab, _model, StateSize, _beginId, _endId);

    private IntState BeginState()
    {
        var ids = new int[StateSize];
        Array.Fill(ids, _beginId);
        return new IntState(ids);
    }

    private int MoveId(IntState state, Random rng)
    {
        if (!_cache.TryGetValue(state, out var entry))
        {
            if (!_model.TryGetValue(state, out var followers))
                throw new KeyNotFoundException("State is not present in the model.");

            var choices = new int[followers.Count];
            var cumDist = new double[followers.Count];
            var sum = 0.0;
            var index = 0;
            foreach (var (id, count) in followers)
            {
                choices[index] = id;
                sum += _temperature.Equals(1.0) ? count : Math.Pow(count, 1.0 / _temperature);
                cumDist[index] = sum;
                index++;
            }

            entry = (choices, cumDist);
            _cache[state] = entry;
        }

        var roll = rng.NextDouble() * entry.CumDist[^1];
        var pick = BisectRight(entry.CumDist, roll);
        return entry.Choices[pick];
    }

    /// <summary>Walks the chain, yielding generated words up to (not including) the end sentinel.</summary>
    public IEnumerable<string> Walk(Random? rng = null)
    {
        rng ??= Random.Shared;
        var state = BeginState();
        while (true)
        {
            var next = MoveId(state, rng);
            if (next == _endId)
                yield break;
            yield return _vocab.Word(next);
            state = state.Shift(next);
        }
    }

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
