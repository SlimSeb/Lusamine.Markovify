namespace Lusamine.Markovify.Benchmarks;

/// <summary>
/// A read-only chain in compressed-sparse-row (CSR) form, frozen from an
/// <see cref="IntChain"/>. The ~N per-state <c>Dictionary</c> objects are replaced by
/// a handful of flat arrays:
/// <list type="bullet">
/// <item><c>_index</c>: the one remaining hash map, state -&gt; row number;</item>
/// <item><c>_rowStart[r]..rowStart[r+1]</c>: the slice of followers for row <c>r</c>;</item>
/// <item><c>_followerIds</c> / <c>_followerCounts</c>: every transition, laid out contiguously;</item>
/// <item><c>_cumWeights</c>: per-row cumulative sampling weights, precomputed for the
/// current temperature so a walk never allocates or re-sums on the hot path.</item>
/// </list>
/// Deleting the per-state dictionaries is the memory lever; the contiguous,
/// alloc-free walk is the speed lever.
/// </summary>
internal sealed class FrozenIntChain
{
    private readonly Vocabulary _vocab;
    private readonly Dictionary<IntState, int> _index;
    private readonly int[] _rowStart;
    private readonly int[] _followerIds;
    private readonly int[] _followerCounts;
    private double[] _cumWeights;
    private readonly int _beginId;
    private readonly int _endId;
    private double _temperature = 1.0;

    public int StateSize { get; }

    public int StateCount => _index.Count;

    /// <summary>Sum of all follower counts (for sanity-checking against the source chain).</summary>
    public long TotalTransitions
    {
        get
        {
            long sum = 0;
            foreach (var c in _followerCounts)
                sum += c;
            return sum;
        }
    }

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
            _cumWeights = BuildCumWeights();
        }
    }

    internal FrozenIntChain(Vocabulary vocab, Dictionary<IntState, Dictionary<int, int>> model,
        int stateSize, int beginId, int endId)
    {
        _vocab = vocab;
        StateSize = stateSize;
        _beginId = beginId;
        _endId = endId;

        var rows = model.Count;
        var total = 0;
        foreach (var followers in model.Values)
            total += followers.Count;

        _index = new Dictionary<IntState, int>(rows);
        _rowStart = new int[rows + 1];
        _followerIds = new int[total];
        _followerCounts = new int[total];

        var row = 0;
        var pos = 0;
        foreach (var (state, followers) in model)
        {
            _index[state] = row;
            _rowStart[row] = pos;
            foreach (var (id, count) in followers)
            {
                _followerIds[pos] = id;
                _followerCounts[pos] = count;
                pos++;
            }
            row++;
        }
        _rowStart[rows] = pos;
        _cumWeights = BuildCumWeights();
    }

    private double[] BuildCumWeights()
    {
        var cum = new double[_followerCounts.Length];
        var atOne = _temperature.Equals(1.0);
        var rows = _rowStart.Length - 1;
        for (var r = 0; r < rows; r++)
        {
            var sum = 0.0;
            for (var k = _rowStart[r]; k < _rowStart[r + 1]; k++)
            {
                var count = _followerCounts[k];
                sum += atOne ? count : Math.Pow(count, 1.0 / _temperature);
                cum[k] = sum;
            }
        }
        return cum;
    }

    private IntState BeginState()
    {
        var ids = new int[StateSize];
        Array.Fill(ids, _beginId);
        return new IntState(ids);
    }

    public IEnumerable<string> Walk(Random? rng = null)
    {
        rng ??= Random.Shared;
        var state = BeginState();
        while (true)
        {
            if (!_index.TryGetValue(state, out var row))
                throw new KeyNotFoundException("State is not present in the model.");

            var start = _rowStart[row];
            var end = _rowStart[row + 1];
            var roll = rng.NextDouble() * _cumWeights[end - 1];
            var pick = BisectRight(_cumWeights, start, end, roll);
            var next = _followerIds[pick];

            if (next == _endId)
                yield break;
            yield return _vocab.Word(next);
            state = state.Shift(next);
        }
    }

    // bisect_right within the half-open row slice [lo, hi).
    private static int BisectRight(double[] cumulative, int lo, int hi, double value)
    {
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (value < cumulative[mid])
                hi = mid;
            else
                lo = mid + 1;
        }
        return lo;
    }
}
