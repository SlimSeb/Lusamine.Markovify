using BenchmarkDotNet.Running;
using Lusamine.Markovify;
using Lusamine.Markovify.Benchmarks;

// Modes:
//   (default) check     - verify IntChain is structurally equivalent to Chain
//   memory <string|int|frozen> - measure ONE built model's retained size in this
//                         process, so each variant gets a clean, uncontaminated heap.
//                         Run each variant as its own process and compare.
//   bench               - run the BenchmarkDotNet suite (use -c Release)
var mode = args.Length > 0 ? args[0] : "check";

switch (mode)
{
    case "bench":
        BenchmarkRunner.Run<ChainBenchmarks>();
        break;
    case "memory":
        MemoryOne(args.Length > 1 ? args[1] : "string");
        break;
    default:
        Check();
        break;
}

return;

// Confirms the two representations encode the same model, so the benchmark compares
// equivalent work rather than two different computations.
static void Check()
{
    var corpus = Corpus.Generate(sentences: 20_000, vocabSize: 4_000, minWords: 6, maxWords: 16, seed: 1);

    foreach (var stateSize in new[] { 1, 2, 3 })
    {
        var chain = Chain.Build(corpus, stateSize);
        var intChain = IntChain.Build(corpus, stateSize);
        var frozen = IntChain.Build(corpus, stateSize).Freeze();

        // Total transitions (sum of all follower counts) must match exactly.
        long stringTotal = chain.Model.Values.Sum(f => (long)f.Values.Sum());

        if (chain.Model.Count != intChain.StateCount || chain.Model.Count != frozen.StateCount)
            throw new Exception($"State count mismatch at stateSize {stateSize}: " +
                                $"{chain.Model.Count} / {intChain.StateCount} / {frozen.StateCount}");
        if (frozen.TotalTransitions != stringTotal)
            throw new Exception($"Transition count mismatch at stateSize {stateSize}: " +
                                $"{stringTotal} vs frozen {frozen.TotalTransitions}");

        Console.WriteLine($"stateSize {stateSize}: states={chain.Model.Count:N0}, " +
                          $"transitions={stringTotal:N0} -- OK (string == int == frozen)");
    }

    Console.WriteLine("\nStructural equivalence check passed.");
    Console.WriteLine("Run `dotnet run -c Release -- bench` for timings, or `-- memory` for resident size.");
}

// Measures one variant's retained model size with a clean baseline. The corpus is
// generated, the chain built, then the corpus is dropped and the heap compacted so
// only the finished model survives. This is what exposes the vocabulary's real win:
// duplicate word objects the model no longer references get collected.
static void MemoryOne(string variant)
{
    const int sentences = 100_000;
    const int stateSize = 2;

    GC.Collect();
    GC.WaitForPendingFinalizers();
    Compact();
    var baseline = GC.GetTotalMemory(forceFullCollection: true);
    var beforeAlloc = GC.GetTotalAllocatedBytes(precise: true);

    var corpus = Corpus.Generate(sentences, vocabSize: 8_000, minWords: 6, maxWords: 18, seed: 99);

    object model;
    int states;
    switch (variant)
    {
        case "int":
        {
            var c = IntChain.Build(corpus, stateSize);
            model = c;
            states = c.StateCount;
            break;
        }
        case "frozen":
        {
            // Build then freeze; the intermediate IntChain is dropped before measuring.
            var c = IntChain.Build(corpus, stateSize).Freeze();
            model = c;
            states = c.StateCount;
            break;
        }
        default:
        {
            var c = Chain.Build(corpus, stateSize);
            model = c;
            states = c.Model.Count;
            break;
        }
    }

    // Drop the corpus and everything transient; keep only the built model.
    corpus = null;
    Compact();

    var allocated = GC.GetTotalAllocatedBytes(precise: true) - beforeAlloc;
    var retained = GC.GetTotalMemory(forceFullCollection: true) - baseline;
    GC.KeepAlive(model);

    var label = variant switch
    {
        "int" => "IntChain (int ids)",
        "frozen" => "FrozenIntChain (CSR)",
        _ => "Chain (string keys)"
    };
    Console.WriteLine($"{label,-22} states={states,9:N0}  retained={retained / (1024.0 * 1024.0),7:F1}MB" +
                      $"  ({(double)retained / states:F0} B/state)  allocated={allocated / (1024.0 * 1024.0),7:F1}MB");
}

static void Compact()
{
    System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
        System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
}
