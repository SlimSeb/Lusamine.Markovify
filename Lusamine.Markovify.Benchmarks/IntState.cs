namespace Lusamine.Markovify.Benchmarks;

/// <summary>
/// The integer-id equivalent of <see cref="State"/>: a window of word ids.
/// Two cheap wins over the string-backed original:
/// <list type="bullet">
/// <item>equality and hashing compare 4-byte ints, not multi-char strings;</item>
/// <item>the hash is computed once at construction and cached, so the hot
/// dictionary lookups during <c>Walk</c> never re-hash the key.</item>
/// </list>
/// For state sizes of 1 or 2 this struct could be replaced entirely by a single
/// packed <see cref="long"/> key (two 32-bit ids), removing the per-state array
/// allocation as well; that is the next lever if these numbers warrant it.
/// </summary>
internal readonly struct IntState : IEquatable<IntState>
{
    private readonly int[] _ids;
    private readonly int _hash;

    public IntState(int[] ids)
    {
        _ids = ids;
        var hash = new HashCode();
        foreach (var id in ids)
            hash.Add(id);
        _hash = hash.ToHashCode();
    }

    public int[] Ids => _ids;

    public IntState Shift(int next)
    {
        var shifted = new int[_ids.Length];
        Array.Copy(_ids, 1, shifted, 0, _ids.Length - 1);
        shifted[^1] = next;
        return new IntState(shifted);
    }

    public bool Equals(IntState other)
    {
        if (_hash != other._hash || _ids.Length != other._ids.Length)
            return false;
        for (var i = 0; i < _ids.Length; i++)
        {
            if (_ids[i] != other._ids[i])
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is IntState other && Equals(other);

    public override int GetHashCode() => _hash;
}
