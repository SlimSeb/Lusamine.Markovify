# Lusamine.Markovify

A [markovify](https://github.com/jsvine/markovify)-style Markov chain text generator for .NET. Train a model from a text corpus, then generate new sentences that resemble the source without copying it verbatim.

- **Weighted sampling** : next-word choices are weighted by how often they were observed, with an optional **temperature** to flatten the distribution.
- **Rejection sampling** : generated sentences that overlap the source too much are discarded.
- **Normalization** : optionally lowercase and strip punctuation before training to merge equivalent tokens.
- **Constrained generation** : limit by length, word count, or a required opening.
- **Model combining** : blend several trained models with weights.
- **JSON serialization** : persist and reload trained models.
- **Pluggable tokenization** : punctuation-aware sentence splitting, or one-sentence-per-line.

Targets **.NET 10**. No third-party dependencies.

## Installation

```bash
dotnet add package Lusamine.Markovify
```

## Quick start

```csharp
using Lusamine.Markovify;

string corpus = File.ReadAllText("corpus.txt");

// Train a 2nd-order model (each state = 2 words).
var model = new Text(corpus, stateSize: 2);

// Generate a sentence (null if no acceptable sentence was found).
string? sentence = model.MakeSentence();
Console.WriteLine(sentence);
```

## Generating text

```csharp
// A sentence no longer than 140 characters.
string? tweet = model.MakeShortSentence(maxChars: 140);

// A sentence that starts with specific words.
string? opener = model.MakeSentenceWithStart("The cat");

// Loosely: start anywhere whose words begin with "cat".
string? loose = model.MakeSentenceWithStart("cat", strict: false);

// Bound the number of words.
string? bounded = model.MakeSentence(minWords: 6, maxWords: 20);
```

### Why `MakeSentence` can return `null`

By default, generated sentences are **rejection-tested**: if too much of the
sentence is copied verbatim from the source, it is rejected and another attempt
is made (up to `tries`, default 10). With a very small corpus, *every* possible
sentence reproduces the source, so all attempts fail and you get `null`. Use a
larger corpus, raise `tries`, relax the overlap thresholds, or disable the test:

```csharp
string? raw = model.MakeSentence(testOutput: false);

string? looser = model.MakeSentence(
    tries: 50,
    maxOverlapRatio: 0.85,   // default 0.7
    maxOverlapTotal: 20);    // default 15
```

## State size

`stateSize` is the Markov order — the number of previous words used to pick the
next one. Larger values produce text that more closely mirrors the source (and
is more likely to reproduce it verbatim); smaller values are more random.

```csharp
var loose  = new Text(corpus, stateSize: 1);
var tight  = new Text(corpus, stateSize: 3);
```

## Reducing verbatim reproduction

Beyond state size and the rejection test, two options help the model produce
text that copies the source less.

### Normalization

Pass `normalize: true` to lowercase each word and strip non-alphanumeric
characters before training. Tokens like `"Hello,"` and `"hello"` then collapse
into one, so each state has a richer set of followers (less deterministic, less
sparse). Tokens that become empty after stripping are dropped.

```csharp
var model = new Text(corpus, stateSize: 2, normalize: true);
```

Note that normalization discards casing and punctuation, so generated sentences
will be lowercased and unpunctuated.

### Temperature

`temperature` reshapes the sampling distribution. At `1.0` (the default) the
trained counts are used as-is. Values **greater than 1.0** flatten the
distribution, favoring rarer transitions and making the walk less likely to
follow a single memorized path; values in `(0, 1)` sharpen it toward the most
common follower.

```csharp
// Flatter sampling: less verbatim copying. Try 1.3-1.8; very high values
// trend toward gibberish.
var model = new Text(corpus, stateSize: 2, temperature: 1.5);

// Also adjustable after construction (works on FromJson / Combine results too).
model.Chain.Temperature = 2.0;
```

Because temperature makes the model *produce* less copied text, fewer
generations get rejected by the overlap test, so generation yield improves.

## One sentence per line

Use `NewlineText` when each line of input is its own unit (tweets, song lines,
headlines) rather than punctuation-delimited prose:

```csharp
var model = new NewlineText(linesOfText, stateSize: 2);
```

## Combining models

```csharp
var a = new Text(corpusA);
var b = new Text(corpusB);

// Weight b twice as heavily as a.
var blended = Text.Combine([a, b], [1.0, 2.0]);
```

## Saving and loading

Save and load straight to a file:

```csharp
model.Save("model.json");

var reloaded = Text.Load("model.json");

// Async variants are available too:
await model.SaveAsync("model.json");
var loaded = await Text.LoadAsync("model.json");
```

Or work with the JSON yourself:

```csharp
string json = model.ToJson();
File.WriteAllText("model.json", json);

var reloaded = Text.FromJson(File.ReadAllText("model.json"));
```

> Serialization stores the chain, state size, and sampling temperature, not the original corpus, so a
> reloaded model cannot rejection-test against the source. Call generation
> methods with `testOutput: false` on reloaded models, or retrain to restore it.

## Working with the chain directly

`Text` wraps a lower-level `Chain`. You can use it on its own for non-text
sequences:

```csharp
var corpus = new[]
{
    new[] { "red", "green", "blue" },
    new[] { "red", "blue", "green" },
};

var chain = Chain.Build(corpus, stateSize: 1);
var rng = new Random();

string[] generated = chain.Walk(rng: rng).ToArray();
```

## Reproducibility

Pass your own `Random` to make output deterministic:

```csharp
var model = new Text(corpus, stateSize: 2, rng: new Random(seed: 42));
```

## Custom tokenization

Subclass `Text` and override `WordSplit` / `WordJoin` to change how sentences
are tokenized and reassembled (for example, to treat punctuation as separate
tokens). Override the sentence splitter by subclassing and supplying your own
parsed sentences to the protected constructor.

## API summary

| Type | Purpose |
| --- | --- |
| `Text` | High-level model: train from text, generate sentences. |
| `NewlineText` | `Text` variant where each line is one sentence. |
| `Chain` | Low-level weighted Markov chain over token sequences. |
| `State` | Immutable, value-equatable window of words (a chain key). |
| `Splitters` | Default sentence- and word-splitting helpers. |

Key `Text` members: `MakeSentence`, `MakeShortSentence`,
`MakeSentenceWithStart`, `Save` / `Load`, `ToJson` / `FromJson`, `Combine`,
`Chain`, `ParsedSentences`. Key `Chain` members: `Build`, `Walk`, `Combine`,
`Temperature`, `ToJson` / `FromJson`.

## Project layout

```
Lusamine.Markovify/         the library
Lusamine.Markovify.Tests/   xUnit test suite
Lusamine.Markovify.Sample/  runnable console sample
```

Run the sample:

```bash
dotnet run --project Lusamine.Markovify.Sample
```

Run the tests:

```bash
dotnet test
```

## License

MIT.
