using System.Text;
using System.Text.Json;

namespace Lusamine.Markovify;

/// <summary>
/// A high-level Markov text generator. It tokenizes a corpus into sentences and
/// words, trains a <see cref="Chain"/>, and generates new sentences using
/// rejection sampling to avoid reproducing the source text verbatim.
/// </summary>
public class Text
{
    /// <summary>Default number of generation attempts before giving up.</summary>
    public const int DefaultTries = 10;

    /// <summary>Default maximum fraction of a sentence that may overlap the source.</summary>
    public const double DefaultMaxOverlapRatio = 0.7;

    /// <summary>Default maximum number of consecutive words that may overlap the source.</summary>
    public const int DefaultMaxOverlapTotal = 15;

    private readonly Random _rng;
    private readonly string? _rejoinedText;

    /// <summary>The trained underlying chain.</summary>
    public Chain Chain { get; }

    /// <summary>The chain's state size.</summary>
    public int StateSize => Chain.StateSize;

    /// <summary>The tokenized source sentences, if the original text was retained.</summary>
    public IReadOnlyList<IReadOnlyList<string>>? ParsedSentences { get; }

    /// <summary>
    /// Trains a model from <paramref name="inputText"/>.
    /// </summary>
    /// <param name="inputText">The corpus to learn from.</param>
    /// <param name="stateSize">Number of words per state (the Markov order).</param>
    /// <param name="retainOriginal">
    /// When <c>true</c> (default), the source is kept so generated sentences can be
    /// rejection-tested for excessive overlap with the original.
    /// </param>
    /// <param name="rng">Random source; defaults to <see cref="Random.Shared"/>.</param>
    /// <param name="normalize">
    /// When <c>true</c>, each word is lowercased and stripped of non-alphanumeric
    /// characters before training, so tokens like <c>"Hello,"</c> and <c>"hello"</c>
    /// are treated as the same word.
    /// </param>
    /// <param name="temperature">
    /// Sampling temperature for generation. <c>1.0</c> (default) reproduces the
    /// trained distribution; values greater than <c>1.0</c> reduce verbatim copying
    /// of the source by favoring rarer transitions. See <see cref="Chain.Temperature"/>.
    /// </param>
    public Text(string inputText, int stateSize = 2, bool retainOriginal = true, Random? rng = null,
        bool normalize = false, double temperature = 1.0)
        : this(ParseSentencesStatic(inputText, normalize), stateSize, retainOriginal, rng,
            chain: null, sentenceSplitterUsed: true)
    {
        Chain.Temperature = temperature;
    }

    /// <summary>
    /// Constructs a model from already-tokenized sentences and/or a pre-built chain.
    /// </summary>
    protected Text(
        IReadOnlyList<IReadOnlyList<string>> parsedSentences,
        int stateSize,
        bool retainOriginal,
        Random? rng,
        Chain? chain,
        bool sentenceSplitterUsed)
    {
        _ = sentenceSplitterUsed;
        _rng = rng ?? Random.Shared;
        Chain = chain ?? Chain.Build(parsedSentences, stateSize);

        if (retainOriginal)
        {
            ParsedSentences = parsedSentences;
            var builder = new StringBuilder();
            foreach (var sentence in parsedSentences)
            {
                builder.Append(WordJoin(sentence));
                builder.Append(' ');
            }

            _rejoinedText = builder.ToString();
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseSentencesStatic(string inputText, bool normalize = false)
    {
        var sentences = new List<IReadOnlyList<string>>();
        foreach (var sentence in Splitters.SplitIntoSentences(inputText))
        {
            var words = Splitters.SplitIntoWords(sentence, normalize);
            if (words.Count > 0)
                sentences.Add(words);
        }

        return sentences;
    }

    /// <summary>Splits a sentence into words. Override to customize tokenization.</summary>
    protected virtual IReadOnlyList<string> WordSplit(string sentence) => Splitters.SplitIntoWords(sentence);

    /// <summary>Joins words back into a sentence. Override to match <see cref="WordSplit"/>.</summary>
    protected virtual string WordJoin(IReadOnlyList<string> words) => string.Join(' ', words);

    /// <summary>
    /// Generates a single sentence, or <c>null</c> if no acceptable sentence was
    /// produced within <paramref name="tries"/> attempts.
    /// </summary>
    public string? MakeSentence(
        State? initState = null,
        int tries = DefaultTries,
        bool testOutput = true,
        int maxOverlapTotal = DefaultMaxOverlapTotal,
        double maxOverlapRatio = DefaultMaxOverlapRatio,
        int? maxWords = null,
        int? minWords = null)
    {
        var prefix = new List<string>();
        if (initState is { } start)
        {
            foreach (var word in start.Words)
            {
                if (word != Chain.Begin)
                    prefix.Add(word);
            }
        }

        for (var attempt = 0; attempt < tries; attempt++)
        {
            List<string> words;
            try
            {
                words = new List<string>(prefix);
                words.AddRange(Chain.Walk(initState, _rng));
            }
            catch (KeyNotFoundException)
            {
                // The supplied init state isn't in the model; nothing to generate.
                return null;
            }

            if (maxWords is { } max && words.Count > max)
                continue;
            if (minWords is { } min && words.Count < min)
                continue;

            if (testOutput && _rejoinedText != null)
            {
                if (!TestSentenceOutput(words, maxOverlapTotal, maxOverlapRatio))
                    continue;
            }

            return WordJoin(words);
        }

        return null;
    }

    /// <summary>
    /// Generates a sentence whose length is within the given character bounds,
    /// or <c>null</c> if none was found.
    /// </summary>
    public string? MakeShortSentence(
        int maxChars,
        int minChars = 0,
        int tries = DefaultTries,
        bool testOutput = true,
        int maxOverlapTotal = DefaultMaxOverlapTotal,
        double maxOverlapRatio = DefaultMaxOverlapRatio)
    {
        for (var attempt = 0; attempt < tries; attempt++)
        {
            var sentence = MakeSentence(
                tries: 1,
                testOutput: testOutput,
                maxOverlapTotal: maxOverlapTotal,
                maxOverlapRatio: maxOverlapRatio);

            if (sentence != null && sentence.Length <= maxChars && sentence.Length >= minChars)
                return sentence;
        }

        return null;
    }

    /// <summary>
    /// Generates a sentence that begins with <paramref name="beginning"/>.
    /// </summary>
    /// <param name="beginning">The desired opening words.</param>
    /// <param name="strict">
    /// When <c>true</c>, the beginning must align exactly with a state boundary.
    /// When <c>false</c>, any state whose words start with the beginning is eligible.
    /// </param>
    /// <param name="tries">Number of generation attempts per candidate start state.</param>
    /// <param name="testOutput">Whether to reject output that overlaps the source too much.</param>
    /// <param name="maxOverlapTotal">Maximum number of consecutive overlapping words allowed.</param>
    /// <param name="maxOverlapRatio">Maximum fraction of the sentence allowed to overlap the source.</param>
    public string? MakeSentenceWithStart(
        string beginning,
        bool strict = true,
        int tries = DefaultTries,
        bool testOutput = true,
        int maxOverlapTotal = DefaultMaxOverlapTotal,
        double maxOverlapRatio = DefaultMaxOverlapRatio)
    {
        var split = WordSplit(beginning);
        var wordCount = split.Count;

        List<State> initStates;
        if (wordCount == StateSize)
        {
            initStates = new List<State> { new(split.ToArray()) };
        }
        else if (wordCount > 0 && wordCount < StateSize)
        {
            if (strict)
            {
                var words = new string[StateSize];
                var pad = StateSize - wordCount;
                for (var i = 0; i < pad; i++)
                    words[i] = Chain.Begin;
                for (var i = 0; i < wordCount; i++)
                    words[pad + i] = split[i];
                initStates = [new State(words)];
            }
            else
            {
                initStates = [];
                foreach (var state in Chain.Model.Keys)
                {
                    var nonBegin = state.Words.Where(w => w != Chain.Begin).ToArray();
                    if (nonBegin.Length < wordCount)
                        continue;
                    var matches = true;
                    for (var i = 0; i < wordCount; i++)
                    {
                        if (!string.Equals(nonBegin[i], split[i], StringComparison.Ordinal))
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (matches)
                        initStates.Add(state);
                }

                Shuffle(initStates);
            }
        }
        else
        {
            throw new ArgumentException(
                $"`beginning` must contain between 1 and {StateSize} words; got {wordCount}.",
                nameof(beginning));
        }

        foreach (var initState in initStates)
        {
            var output = MakeSentence(
                initState,
                tries,
                testOutput,
                maxOverlapTotal,
                maxOverlapRatio);
            if (output != null)
                return output;
        }

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="words"/> does not overlap the source
    /// text by more than the configured thresholds. Mirrors markovify's logic.
    /// </summary>
    protected bool TestSentenceOutput(IReadOnlyList<string> words, int maxOverlapTotal, double maxOverlapRatio)
    {
        if (_rejoinedText == null)
            return true;

        var overlapRatio = (int)Math.Round(maxOverlapRatio * words.Count, MidpointRounding.AwayFromZero);
        var overlapMax = Math.Min(maxOverlapTotal, overlapRatio);
        var overlapOver = overlapMax + 1;
        var gramCount = Math.Max(words.Count - overlapMax, 1);

        for (var i = 0; i < gramCount; i++)
        {
            var take = Math.Min(overlapOver, words.Count - i);
            var gram = new string[take];
            for (var j = 0; j < take; j++)
                gram[j] = words[i + j];

            var gramJoined = WordJoin(gram);
            if (_rejoinedText.Contains(gramJoined, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>Serializes the model (state size + chain) to JSON.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("state_size", StateSize);
            writer.WritePropertyName("chain");
            writer.WriteRawValue(Chain.ToJson());
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reconstructs a model previously produced by <see cref="ToJson"/>.</summary>
    public static Text FromJson(string json, Random? rng = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var stateSize = root.GetProperty("state_size").GetInt32();
        var chain = Chain.FromJson(root.GetProperty("chain").GetRawText());
        return new Text(
            Array.Empty<IReadOnlyList<string>>(),
            stateSize,
            retainOriginal: false,
            rng,
            chain,
            sentenceSplitterUsed: true);
    }

    /// <summary>
    /// Combines several models into one by combining their chains. Retained source
    /// sentences (when present in every model) are concatenated for overlap testing.
    /// </summary>
    public static Text Combine(IReadOnlyList<Text> models, IReadOnlyList<double>? weights = null, Random? rng = null)
    {
        if (models.Count == 0)
            throw new ArgumentException("At least one model is required.", nameof(models));

        var chain = Chain.Combine(models.Select(m => m.Chain).ToArray(), weights);

        var allParsed = new List<IReadOnlyList<string>>();
        var retain = true;
        foreach (var model in models)
        {
            if (model.ParsedSentences == null)
            {
                retain = false;
                break;
            }

            allParsed.AddRange(model.ParsedSentences);
        }

        return new Text(
            retain ? allParsed : Array.Empty<IReadOnlyList<string>>(),
            chain.StateSize,
            retainOriginal: retain,
            rng,
            chain,
            sentenceSplitterUsed: true);
    }

    private void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}