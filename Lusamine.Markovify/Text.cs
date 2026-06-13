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

    // The retained source is held as bounded chunks rather than one contiguous string:
    // a corpus large enough to exceed .NET's maximum string length would otherwise throw
    // when rejoined. Consecutive chunks overlap by RejoinedChunkOverlap characters so an
    // overlap gram that straddles a chunk boundary is still wholly contained in one chunk.
    // These are mutable only so tests can force many small chunks without a huge corpus.
    internal static int RejoinedChunkLimit = 64 * 1024 * 1024;
    internal static int RejoinedChunkOverlap = 64 * 1024;

    private readonly Random _rng;
    private readonly IReadOnlyList<string>? _rejoinedChunks;

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
            _rejoinedChunks = BuildRejoinedChunks(parsedSentences);
        }
    }

    // Joins the source sentences back into space-separated text, split across chunks no
    // larger than RejoinedChunkLimit. Each new chunk is seeded with the tail of the
    // previous one so a gram spanning the cut is still found by RejoinedContains.
    private IReadOnlyList<string> BuildRejoinedChunks(IReadOnlyList<IReadOnlyList<string>> parsedSentences)
    {
        var chunks = new List<string>();
        var builder = new StringBuilder();
        foreach (var sentence in parsedSentences)
        {
            builder.Append(WordJoin(sentence));
            builder.Append(' ');

            if (builder.Length >= RejoinedChunkLimit)
            {
                var chunk = builder.ToString();
                chunks.Add(chunk);
                builder.Clear();
                var keep = Math.Min(RejoinedChunkOverlap, chunk.Length);
                builder.Append(chunk, chunk.Length - keep, keep);
            }
        }

        if (builder.Length > 0)
            chunks.Add(builder.ToString());

        return chunks;
    }

    // Whether the retained source contains the given text, searching each chunk.
    private bool RejoinedContains(string value)
    {
        if (_rejoinedChunks == null)
            return false;
        foreach (var chunk in _rejoinedChunks)
        {
            if (chunk.Contains(value, StringComparison.Ordinal))
                return true;
        }

        return false;
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

    /// <summary>
    /// Builds a model from a stream of already-tokenized sentences, consuming
    /// <paramref name="sentences"/> in a single pass. This is the right entry point for a
    /// corpus too large to hold in memory as one string: pass a lazily-evaluated sequence
    /// (for example <c>File.ReadLines(path).Select(line =&gt; Splitters.SplitIntoWords(line))</c>)
    /// and only one sentence is materialized at a time.
    /// </summary>
    /// <param name="sentences">The tokenized sentences to train on, evaluated lazily.</param>
    /// <param name="stateSize">Number of words per state (the Markov order).</param>
    /// <param name="rng">Random source; defaults to <see cref="Random.Shared"/>.</param>
    /// <param name="temperature">
    /// Sampling temperature for generation. See <see cref="Chain.Temperature"/>.
    /// </param>
    /// <remarks>
    /// Because the source is streamed and never retained, the resulting model cannot
    /// rejection-test generated sentences against the original. Call generation methods
    /// with <c>testOutput: false</c>.
    /// </remarks>
    public static Text FromSentences(
        IEnumerable<IReadOnlyList<string>> sentences,
        int stateSize = 2,
        Random? rng = null,
        double temperature = 1.0)
    {
        // Drop empty runs so a blank line in the source doesn't create a degenerate state,
        // matching how the string constructors filter tokenized sentences.
        var nonEmpty = sentences.Where(s => s.Count > 0);
        var chain = Chain.Build(nonEmpty, stateSize);
        chain.Temperature = temperature;
        return new Text(
            Array.Empty<IReadOnlyList<string>>(),
            stateSize,
            retainOriginal: false,
            rng,
            chain,
            sentenceSplitterUsed: true);
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

            if (testOutput && _rejoinedChunks != null)
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
        if (_rejoinedChunks == null)
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
            if (RejoinedContains(gramJoined))
                return false;
        }

        return true;
    }

    /// <summary>Serializes the model (state size, sampling temperature, and chain) to JSON.</summary>
    /// <remarks>
    /// Materializes the whole model as a single <see cref="string"/>; for very large
    /// corpora this can exceed the maximum string length. Prefer <see cref="Save(string)"/>,
    /// which streams directly to disk.
    /// </remarks>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteTo(writer);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes the model to <paramref name="writer"/> as a JSON object.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("state_size", StateSize);
        writer.WriteNumber("temperature", Chain.Temperature);
        writer.WritePropertyName("chain");
        Chain.WriteTo(writer);
        writer.WriteEndObject();
    }

    /// <summary>Reconstructs a model previously produced by <see cref="ToJson"/>.</summary>
    public static Text FromJson(string json, Random? rng = null)
    {
        using var document = JsonDocument.Parse(json);
        return FromElement(document.RootElement, rng);
    }

    private static Text FromElement(JsonElement root, Random? rng)
    {
        var stateSize = root.GetProperty("state_size").GetInt32();
        var chain = Chain.FromElement(root.GetProperty("chain"));
        // Temperature was added in a later format version; default to 1.0 when absent.
        if (root.TryGetProperty("temperature", out var temperature))
            chain.Temperature = temperature.GetDouble();
        return new Text(
            Array.Empty<IReadOnlyList<string>>(),
            stateSize,
            retainOriginal: false,
            rng,
            chain,
            sentenceSplitterUsed: true);
    }

    /// <summary>Saves the model to <paramref name="path"/> as JSON, overwriting any existing file.</summary>
    /// <remarks>Streams directly to disk, so it handles models too large to hold in a single string.</remarks>
    public void Save(string path)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream);
        WriteTo(writer);
    }

    /// <summary>Saves the model to <paramref name="path"/> as JSON, overwriting any existing file.</summary>
    /// <remarks>Streams directly to disk, so it handles models too large to hold in a single string.</remarks>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = File.Create(path);
        await using (stream.ConfigureAwait(false))
        {
            var writer = new Utf8JsonWriter(stream);
            await using (writer.ConfigureAwait(false))
            {
                WriteTo(writer);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Loads a model previously written by <see cref="Save"/>.</summary>
    /// <remarks>Parses directly from the file, so it handles models too large to hold in a single string.</remarks>
    public static Text Load(string path, Random? rng = null)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return FromElement(document.RootElement, rng);
    }

    /// <summary>Loads a model previously written by <see cref="Save"/>.</summary>
    /// <remarks>Parses directly from the file, so it handles models too large to hold in a single string.</remarks>
    public static async Task<Text> LoadAsync(string path, Random? rng = null,
        CancellationToken cancellationToken = default)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return FromElement(document.RootElement, rng);
        }
    }

    // Identifies the binary model format and guards against feeding in an unrelated file.
    // Bytes spell "LMKV"; bumped only if the layout written by WriteBinary changes.
    private static readonly byte[] BinaryMagic = "LMKV"u8.ToArray();
    private const byte BinaryVersion = 1;

    /// <summary>
    /// Writes the model (state size, sampling temperature, and chain) to
    /// <paramref name="writer"/> in a compact binary form.
    /// </summary>
    /// <remarks>
    /// The binary format is typically several times smaller than the JSON produced by
    /// <see cref="WriteTo"/> and faster to load, at the cost of not being human readable.
    /// It is streamed straight to the writer, so it handles models of any size. Prefer
    /// <see cref="SaveBinary(string)"/> to write directly to a file.
    /// </remarks>
    public void WriteBinary(BinaryWriter writer)
    {
        writer.Write(BinaryMagic);
        writer.Write(BinaryVersion);
        writer.Write(Chain.Temperature);
        Chain.WriteBinary(writer);
    }

    /// <summary>Reconstructs a model previously written by <see cref="WriteBinary"/>.</summary>
    public static Text ReadBinary(BinaryReader reader, Random? rng = null)
    {
        var magic = reader.ReadBytes(BinaryMagic.Length);
        if (!magic.AsSpan().SequenceEqual(BinaryMagic))
            throw new InvalidDataException("Stream does not contain a binary Markovify model.");

        var version = reader.ReadByte();
        if (version != BinaryVersion)
            throw new InvalidDataException($"Unsupported binary model version {version}; expected {BinaryVersion}.");

        var temperature = reader.ReadDouble();
        var chain = Chain.ReadBinary(reader);
        chain.Temperature = temperature;

        return new Text(
            Array.Empty<IReadOnlyList<string>>(),
            chain.StateSize,
            retainOriginal: false,
            rng,
            chain,
            sentenceSplitterUsed: true);
    }

    /// <summary>
    /// Saves the model to <paramref name="path"/> in the compact binary format,
    /// overwriting any existing file.
    /// </summary>
    /// <remarks>Streams directly to disk, so it handles models too large to hold in a single string.</remarks>
    public void SaveBinary(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        WriteBinary(writer);
    }

    /// <summary>
    /// Saves the model to <paramref name="path"/> in the compact binary format,
    /// overwriting any existing file.
    /// </summary>
    /// <remarks>Streams directly to disk, so it handles models too large to hold in a single string.</remarks>
    public async Task SaveBinaryAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = File.Create(path);
        await using (stream.ConfigureAwait(false))
        {
            // BinaryWriter has no async surface, so build the bytes in memory and flush them
            // asynchronously. The buffer mirrors what SaveBinary writes synchronously.
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
                WriteBinary(writer);

            buffer.Position = 0;
            await buffer.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Loads a model previously written by <see cref="SaveBinary"/>.</summary>
    /// <remarks>Reads directly from the file, so it handles models too large to hold in a single string.</remarks>
    public static Text LoadBinary(string path, Random? rng = null)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        return ReadBinary(reader, rng);
    }

    /// <summary>Loads a model previously written by <see cref="SaveBinary"/>.</summary>
    /// <remarks>Reads directly from the file, so it handles models too large to hold in a single string.</remarks>
    public static async Task<Text> LoadBinaryAsync(string path, Random? rng = null,
        CancellationToken cancellationToken = default)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            // BinaryReader is synchronous, so pull the file into memory first, then decode.
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;

            using var reader = new BinaryReader(buffer, Encoding.UTF8);
            return ReadBinary(reader, rng);
        }
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