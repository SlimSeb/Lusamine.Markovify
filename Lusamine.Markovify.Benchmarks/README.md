# Lusamine.Markovify.Benchmarks

A side-by-side experiment comparing the shipping string-keyed `Chain` against an
integer-id reimplementation (`IntChain` + `Vocabulary` + `IntState`). The library
itself is **not** modified; this project references it and reimplements the chain
so the two can be measured head to head.

## What the integer-id version changes

- **`Vocabulary`** interns every distinct word to a small `int` id, stored once.
- **`IntState`** is the int-id equivalent of `State`, and caches its hash code so
  the hot dictionary lookups during a walk never re-hash the key.
- **`IntChain`** stores `state(int ids) -> (follower id -> count)`.

Behaviour is verified equivalent to `Chain` (same state count and same total
transitions) by the `check` mode.

## Running it

```bash
# Structural equivalence check (Debug is fine)
dotnet run -c Release --project Lusamine.Markovify.Benchmarks -- check

# Retained-memory comparison. Each variant runs in its OWN process with a clean,
# compacted heap and the corpus freed before measuring, so the numbers are not
# cross-contaminated. (Running both in one process gives meaningless,
# order-dependent GetTotalMemory deltas.)
dotnet run -c Release --project Lusamine.Markovify.Benchmarks -- memory string
dotnet run -c Release --project Lusamine.Markovify.Benchmarks -- memory int

# Time + allocation via BenchmarkDotNet (Release required)
dotnet run -c Release --project Lusamine.Markovify.Benchmarks -- bench
```

## Results (100k-sentence Zipfian corpus, ~502k states; one machine, short run)

Retained model size (corpus freed, heap compacted):

| Representation     | Retained | Bytes/state |
| ------------------ | -------: | ----------: |
| `Chain` (strings)  | 357 MB   | 745         |
| `IntChain` (ints)  | 261 MB   | 545         |

~27% smaller. BenchmarkDotNet (state size 2 / 3):

| Phase | Metric     | String → Int        |
| ----- | ---------- | ------------------- |
| Build | time       | ~unchanged (wash)   |
| Build | allocated  | 6–10% less          |
| Walk  | time       | ~20% (s2) / ~35% (s3) faster |
| Walk  | allocated  | ~20% less           |

## Reading the results

- **The interesting number is bytes/state: ~600–750 B.** The dominant cost is one
  `Dictionary<,>` object **per state**, and both representations pay it. That is
  why interning keys only buys ~27% rather than the order-of-magnitude you might
  expect.
- **Build is a wash on time:** the per-word `Vocabulary.Intern` lookup roughly
  offsets the cheaper int keys.
- **Walk is the clear win,** and it grows with state size, because the cached hash
  and int comparisons replace the more expensive multi-word string hashing on the
  hot path.

## The bigger lever this exposes

Interning keys is a foundation, not the payoff. The two changes that would move
memory and walk time far more, both *enabled* by integer ids:

1. **Replace the per-state dictionaries with a flat (CSR) layout** after building:
   one `int[]` of follower ids and one `int[]` of counts for the whole model, plus
   a per-state offset. This deletes ~500k `Dictionary` objects.
2. **For state size ≤ 2, pack the state into a single `long` key** (two 32-bit
   ids), removing the per-state `int[]` allocation entirely.
