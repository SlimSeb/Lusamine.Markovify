using System.IO.MemoryMappedFiles;
using System.Text;

namespace Lusamine.Markovify;

/// <summary>
/// A read-only, memory-mapped view of a trained model whose transition table stays on
/// disk and is paged in on demand. Generating touches only the handful of states a walk
/// visits, so resident memory stays in the tens of megabytes no matter how large the file
/// is. This is the way to generate from a multi-gigabyte model on a machine that could
/// never hold the whole chain in RAM.
/// </summary>
/// <remarks>
/// Write the file with <see cref="Text.SaveMapped(string)"/> on a machine large enough to
/// train (or load) the model, copy the file to the constrained machine, then open it here.
/// The original corpus is not stored, so generation cannot rejection-test output against the
/// source. Words are rejoined with single spaces, matching the default <see cref="Text"/>
/// tokenization. Dispose the instance to release the mapping.
/// </remarks>
public sealed class MappedModel : IDisposable
{
    // File layout (all little-endian), produced by Write:
    //   Header (72 bytes): magic, version, stateSize, temperature, stateCount, recordSize,
    //                      stringCount, and absolute offsets to the three sections below.
    //   String index: (stringCount + 1) Int64 byte offsets into the string data block.
    //   String data:  the distinct words, UTF-8, sorted ordinal, concatenated.
    //   State table:  stateCount fixed-size records, sorted by the word-index key, each
    //                 [stateSize * Int32 key][Int64 follower start][Int32 follower count].
    //   Followers:    flat (Int32 word index, Int32 count) records grouped by state.
    private static readonly byte[] Magic = "LMMF"u8.ToArray();
    private const int FormatVersion = 1;
    private const int HeaderSize = 72;
    private const int FollowerRecordSize = 8;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;

    private readonly long _stateCount;
    private readonly int _recordSize;
    private readonly long _stringCount;
    private readonly long _stringIndexOffset;
    private readonly long _stringDataOffset;
    private readonly long _stateTableOffset;
    private readonly long _followerSectionOffset;

    private readonly int _beginIndex;
    private readonly int _endIndex;

    private double _temperature;

    /// <summary>The chain's state size.</summary>
    public int StateSize { get; }

    /// <summary>
    /// Sampling temperature applied when walking. Defaults to the value stored in the file.
    /// See <see cref="Chain.Temperature"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not greater than zero.</exception>
    public double Temperature
    {
        get => _temperature;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Temperature must be greater than zero.");
            _temperature = value;
        }
    }

    private MappedModel(MemoryMappedFile file, MemoryMappedViewAccessor view)
    {
        _file = file;
        _view = view;

        Span<byte> magic = stackalloc byte[Magic.Length];
        for (var i = 0; i < magic.Length; i++)
            magic[i] = _view.ReadByte(i);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("File is not a memory-mapped Markovify model.");

        var version = _view.ReadInt32(4);
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported mapped model version {version}; expected {FormatVersion}.");

        StateSize = _view.ReadInt32(8);
        _temperature = _view.ReadDouble(12);
        _stateCount = _view.ReadInt64(20);
        _recordSize = _view.ReadInt32(28);
        _stringCount = _view.ReadInt64(32);
        _stringIndexOffset = _view.ReadInt64(40);
        _stringDataOffset = _view.ReadInt64(48);
        _stateTableOffset = _view.ReadInt64(56);
        _followerSectionOffset = _view.ReadInt64(64);

        // Resolve the sentinel indices once so the hot path compares ints, not strings.
        _beginIndex = IndexOfWord(Chain.Begin);
        _endIndex = IndexOfWord(Chain.End);
    }

    /// <summary>Opens the memory-mapped model at <paramref name="path"/>.</summary>
    public static MappedModel Open(string path)
    {
        var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0,
            MemoryMappedFileAccess.Read);
        try
        {
            var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            try
            {
                return new MappedModel(file, view);
            }
            catch
            {
                view.Dispose();
                throw;
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Walks the chain from the begin state, yielding generated words until the end
    /// sentinel is reached.
    /// </summary>
    /// <exception cref="KeyNotFoundException">A visited state is missing from the model.</exception>
    public IEnumerable<string> Walk(Random? rng = null)
    {
        rng ??= Random.Shared;
        var key = new int[StateSize];
        Array.Fill(key, _beginIndex);

        while (true)
        {
            var next = Move(key, rng);
            if (next == _endIndex)
                yield break;

            yield return ReadWord(next);

            // Shift the window: drop the oldest index, append the new follower.
            for (var i = 0; i < StateSize - 1; i++)
                key[i] = key[i + 1];
            key[StateSize - 1] = next;
        }
    }

    /// <summary>
    /// Generates a single sentence, or <c>null</c> if none was produced within
    /// <paramref name="tries"/> attempts (or none satisfied the word-count bounds).
    /// </summary>
    public string? MakeSentence(int tries = Text.DefaultTries, int? maxWords = null, int? minWords = null,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        for (var attempt = 0; attempt < tries; attempt++)
        {
            List<string> words;
            try
            {
                words = Walk(rng).ToList();
            }
            catch (KeyNotFoundException)
            {
                return null;
            }

            if (maxWords is { } max && words.Count > max)
                continue;
            if (minWords is { } min && words.Count < min)
                continue;

            return string.Join(' ', words);
        }

        return null;
    }

    /// <summary>
    /// Generates a sentence whose length is within the given character bounds, or
    /// <c>null</c> if none was found.
    /// </summary>
    public string? MakeShortSentence(int maxChars, int minChars = 0, int tries = Text.DefaultTries, Random? rng = null)
    {
        for (var attempt = 0; attempt < tries; attempt++)
        {
            var sentence = MakeSentence(tries: 1, rng: rng);
            if (sentence != null && sentence.Length <= maxChars && sentence.Length >= minChars)
                return sentence;
        }

        return null;
    }

    // Picks the next follower index for the given state key, weighted by count and temperature.
    private int Move(int[] key, Random rng)
    {
        if (!TryFindState(key, out var followerStart, out var followerCount))
            throw new KeyNotFoundException("The current state is not present in the model.");

        var basePos = _followerSectionOffset + followerStart * FollowerRecordSize;

        // Single follower is the common case; skip the cumulative-weight machinery.
        if (followerCount == 1)
            return _view.ReadInt32(basePos);

        var cumulative = new double[followerCount];
        var sum = 0.0;
        for (var i = 0; i < followerCount; i++)
        {
            var count = _view.ReadInt32(basePos + i * FollowerRecordSize + 4);
            sum += _temperature.Equals(1.0) ? count : Math.Pow(count, 1.0 / _temperature);
            cumulative[i] = sum;
        }

        var roll = rng.NextDouble() * sum;
        var pick = BisectRight(cumulative, roll);
        return _view.ReadInt32(basePos + pick * FollowerRecordSize);
    }

    // Binary searches the sorted state table for the given key.
    private bool TryFindState(int[] key, out long followerStart, out int followerCount)
    {
        long lo = 0;
        var hi = _stateCount;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            var cmp = CompareKey(key, _stateTableOffset + mid * _recordSize);
            if (cmp == 0)
            {
                var recordPos = _stateTableOffset + mid * _recordSize;
                followerStart = _view.ReadInt64(recordPos + StateSize * 4);
                followerCount = _view.ReadInt32(recordPos + StateSize * 4 + 8);
                return true;
            }

            if (cmp < 0)
                hi = mid;
            else
                lo = mid + 1;
        }

        followerStart = 0;
        followerCount = 0;
        return false;
    }

    // Compares a key against the state-record key stored at recordPos (lexicographic).
    private int CompareKey(int[] key, long recordPos)
    {
        for (var i = 0; i < StateSize; i++)
        {
            var stored = _view.ReadInt32(recordPos + i * 4);
            if (key[i] != stored)
                return key[i] < stored ? -1 : 1;
        }

        return 0;
    }

    // Resolves a word index to its string via the on-disk string table.
    private string ReadWord(int index)
    {
        var start = _view.ReadInt64(_stringIndexOffset + (long)index * 8);
        var end = _view.ReadInt64(_stringIndexOffset + ((long)index + 1) * 8);
        var length = (int)(end - start);
        if (length == 0)
            return string.Empty;

        var bytes = new byte[length];
        _view.ReadArray(_stringDataOffset + start, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    // Binary searches the sorted string table for a word, returning its index or -1.
    private int IndexOfWord(string word)
    {
        long lo = 0;
        var hi = _stringCount;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            var cmp = string.CompareOrdinal(word, ReadWord((int)mid));
            if (cmp == 0)
                return (int)mid;
            if (cmp < 0)
                hi = mid;
            else
                lo = mid + 1;
        }

        return -1;
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

    /// <inheritdoc />
    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }

    /// <summary>
    /// Writes <paramref name="chain"/> to <paramref name="path"/> in the memory-mapped
    /// format. Run on a machine able to hold the model; the produced file is then openable
    /// with <see cref="Open"/> on a far smaller one.
    /// </summary>
    internal static void Write(Chain chain, double temperature, string path)
    {
        var stateSize = chain.StateSize;

        // Assign every distinct word a sorted-ordinal index, so the on-disk string table
        // supports both index -> word (output) and word -> index (sentinel lookup) lookups.
        var distinct = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (state, followers) in chain.Model)
        {
            foreach (var word in state.Words)
                distinct.Add(word);
            foreach (var word in followers.WordsArray)
                distinct.Add(word);
        }

        var sortedWords = distinct.ToArray();
        var wordToIndex = new Dictionary<string, int>(sortedWords.Length, StringComparer.Ordinal);
        var wordBytes = new byte[sortedWords.Length][];
        for (var i = 0; i < sortedWords.Length; i++)
        {
            wordToIndex[sortedWords[i]] = i;
            wordBytes[i] = Encoding.UTF8.GetBytes(sortedWords[i]);
        }

        // Materialize each state's key as indices and sort the states by that key.
        var states = new (int[] Key, Followers Followers)[chain.Model.Count];
        var s = 0;
        foreach (var (state, followers) in chain.Model)
        {
            var key = new int[stateSize];
            for (var i = 0; i < stateSize; i++)
                key[i] = wordToIndex[state.Words[i]];
            states[s++] = (key, followers);
        }

        Array.Sort(states, (a, b) =>
        {
            for (var i = 0; i < stateSize; i++)
            {
                if (a.Key[i] != b.Key[i])
                    return a.Key[i] < b.Key[i] ? -1 : 1;
            }

            return 0;
        });

        var recordSize = stateSize * 4 + 12;
        long stringDataSize = 0;
        foreach (var bytes in wordBytes)
            stringDataSize += bytes.Length;

        var stringIndexOffset = (long)HeaderSize;
        var stringDataOffset = stringIndexOffset + (sortedWords.Length + 1L) * 8;
        var stateTableOffset = stringDataOffset + stringDataSize;
        var followerSectionOffset = stateTableOffset + (long)states.Length * recordSize;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 20);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        // Header.
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(stateSize);
        writer.Write(temperature);
        writer.Write((long)states.Length);
        writer.Write(recordSize);
        writer.Write((long)sortedWords.Length);
        writer.Write(stringIndexOffset);
        writer.Write(stringDataOffset);
        writer.Write(stateTableOffset);
        writer.Write(followerSectionOffset);

        // String index (cumulative byte offsets) then string data.
        long runningStringOffset = 0;
        foreach (var bytes in wordBytes)
        {
            writer.Write(runningStringOffset);
            runningStringOffset += bytes.Length;
        }

        writer.Write(runningStringOffset);
        foreach (var bytes in wordBytes)
            writer.Write(bytes);

        // State table, recording the running follower start for each state.
        long followerStart = 0;
        foreach (var (key, followers) in states)
        {
            foreach (var index in key)
                writer.Write(index);
            writer.Write(followerStart);
            writer.Write(followers.Count);
            followerStart += followers.Count;
        }

        // Follower records, grouped by state in the same sorted order.
        foreach (var (_, followers) in states)
        {
            var words = followers.WordsArray;
            var counts = followers.CountsArray;
            for (var i = 0; i < words.Length; i++)
            {
                writer.Write(wordToIndex[words[i]]);
                writer.Write(counts[i]);
            }
        }
    }
}
