using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Lusamine.Markovify.Benchmarks;

/// <summary>
/// Head-to-head benchmarks of the shipping string-keyed <see cref="Chain"/> against
/// the integer-id <see cref="IntChain"/>. Run in Release:
/// <c>dotnet run -c Release --project Lusamine.Markovify.Benchmarks -- bench</c>.
/// <para>
/// [MemoryDiagnoser] reports bytes allocated per operation. For the Build benchmarks
/// that allocation total doubles as a proxy for the resident size of the finished
/// model (it is retained by the returned object); for an exact resident-size figure
/// use the <c>memory</c> mode in <see cref="Program"/>.
/// </para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ChainBenchmarks
{
    [Params(2, 3)]
    public int StateSize;

    private IReadOnlyList<IReadOnlyList<string>> _corpus = null!;
    private Chain _chain = null!;
    private IntChain _intChain = null!;
    private FrozenIntChain _frozen = null!;

    [GlobalSetup]
    public void Setup()
    {
        // ~50k sentences, ~5k-word Zipfian vocabulary. Big enough to be representative,
        // small enough that BenchmarkDotNet's repeated invocations stay quick.
        _corpus = Corpus.Generate(sentences: 50_000, vocabSize: 5_000, minWords: 6, maxWords: 18, seed: 42);
        _chain = Chain.Build(_corpus, StateSize);
        _intChain = IntChain.Build(_corpus, StateSize);
        _frozen = IntChain.Build(_corpus, StateSize).Freeze();
    }

    [BenchmarkCategory("Build"), Benchmark(Baseline = true)]
    public int Build_String() => Chain.Build(_corpus, StateSize).Model.Count;

    [BenchmarkCategory("Build"), Benchmark]
    public int Build_Int() => IntChain.Build(_corpus, StateSize).StateCount;

    // Build + freeze: the price paid once to get the CSR layout.
    [BenchmarkCategory("Build"), Benchmark]
    public int Build_Frozen() => IntChain.Build(_corpus, StateSize).Freeze().StateCount;

    [BenchmarkCategory("Walk"), Benchmark(Baseline = true)]
    public int Walk_String()
    {
        var rng = new Random(7);
        var words = 0;
        for (var i = 0; i < 10_000; i++)
            foreach (var _ in _chain.Walk(rng: rng))
                words++;
        return words;
    }

    [BenchmarkCategory("Walk"), Benchmark]
    public int Walk_Int()
    {
        var rng = new Random(7);
        var words = 0;
        for (var i = 0; i < 10_000; i++)
            foreach (var _ in _intChain.Walk(rng))
                words++;
        return words;
    }

    [BenchmarkCategory("Walk"), Benchmark]
    public int Walk_Frozen()
    {
        var rng = new Random(7);
        var words = 0;
        for (var i = 0; i < 10_000; i++)
            foreach (var _ in _frozen.Walk(rng))
                words++;
        return words;
    }
}
