namespace OneImage.Ops.Quantize;

/// <summary>A small open-addressing set of packed RGBA colors that remembers the order colors were first added in.</summary>
internal sealed class ColorSet
{
    private const int Capacity = 1024;
    private readonly uint[] _keys = new uint[Capacity];
    private readonly short[] _slots = new short[Capacity]; // index + 1, 0 = empty
    private readonly uint[] _ordered = new uint[Capacity / 2];

    public int Count { get; private set; }

    /// <summary>Gets the color added <paramref name="index"/>-th.</summary>
    public uint this[int index] => _ordered[index];

    /// <summary>Returns the index of <paramref name="key"/>, or -1.</summary>
    public int Find(uint key)
    {
        int h = (int)((key * 0x9E3779B1u) >> 22);
        while (true)
        {
            int s = _slots[h];
            if (s == 0)
            {
                return -1;
            }

            if (_keys[h] == key)
            {
                return s - 1;
            }

            h = (h + 1) & (Capacity - 1);
        }
    }

    /// <summary>Adds <paramref name="key"/> if absent and returns its index. At most 500 distinct colors may be added.</summary>
    public int Add(uint key)
    {
        int h = (int)((key * 0x9E3779B1u) >> 22);
        while (true)
        {
            int s = _slots[h];
            if (s == 0)
            {
                if (Count >= _ordered.Length)
                {
                    throw new InvalidOperationException("Color set is full.");
                }

                _keys[h] = key;
                _ordered[Count] = key;
                _slots[h] = (short)(++Count);
                return Count - 1;
            }

            if (_keys[h] == key)
            {
                return s - 1;
            }

            h = (h + 1) & (Capacity - 1);
        }
    }
}
