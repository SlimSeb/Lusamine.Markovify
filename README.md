# Lusamine.Markovify

A [markovify](https://github.com/jsvine/markovify)-style Markov chain text generator for .NET. Train a model from a text corpus, then generate new sentences that resemble the source without copying it verbatim.

- **Weighted sampling** : next-word choices are weighted by how often they were observed.
- **Rejection sampling** : generated sentences that overlap the source too much are discarded.
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

```csharp
string json = model.ToJson();
File.WriteAllText("model.json", json);

var reloaded = Text.FromJson(File.ReadAllText("model.json"));
```

> Serialization stores the chain and state size, not the original corpus, so a
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
`MakeSentenceWithStart`, `ToJson` / `FromJson`, `Combine`, `Chain`,
`ParsedSentences`.

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
